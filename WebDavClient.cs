using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LloydScan.Core.Configuration;

namespace LloydScan.Core.Upload;

public sealed class WebDavException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public WebDavException(string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner) => StatusCode = status;
}

/// <summary>Client WebDAV minimal : création des dossiers (MKCOL), dépôt (PUT), vérification (HEAD).</summary>
public sealed class WebDavClient : IDisposable
{
    private readonly WebDavTarget _target;
    private readonly HttpClient _http;
    private readonly Uri _root;
    private readonly HashSet<string> _knownFolders = new(StringComparer.OrdinalIgnoreCase);

    public WebDavClient(WebDavTarget target, WebDavCredential? credential, HttpMessageHandler? handler = null)
    {
        _target = target;
        var url = target.Url.EndsWith('/') ? target.Url : target.Url + "/";
        _root = new Uri(url);

        bool windows = target.Authentication == WebDavAuthentication.Windows;
        if (!windows && credential is null)
            throw new WebDavException($"Identifiants « {Label(target)} » non renseignés : menu « Mes identifiants ».");

        handler ??= new SocketsHttpHandler
        {
            Credentials = windows ? CredentialCache.DefaultCredentials : null,
            PreAuthenticate = true,
            AllowAutoRedirect = false
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(10, target.TimeoutSeconds)) };
        if (!windows)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential!.Username}:{credential.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LloydScan/1.0");
    }

    private static string Label(WebDavTarget t) => string.IsNullOrEmpty(t.DisplayName) ? t.Url : t.DisplayName;

    /// <summary>Vérifie l'accès au dossier racine (PROPFIND Depth 0) : URL joignable et identifiants acceptés.</summary>
    public async Task TestConnectionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _root);
        req.Headers.Add("Depth", "0");
        using var resp = await _http.SendAsync(req, ct);
        if ((int)resp.StatusCode is 207 or 200) return;
        throw new WebDavException(Describe(resp, $"Connexion à « {Label(_target)} »"), resp.StatusCode);
    }

    private Uri Combine(string relative, bool isFolder)
    {
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        var path = string.Join('/', parts);
        if (isFolder && path.Length > 0) path += "/";
        return new Uri(_root, path);
    }

    /// <summary>Crée récursivement le dossier distant s'il n'existe pas.</summary>
    public async Task EnsureFolderAsync(string relativeFolder, CancellationToken ct)
    {
        var segments = relativeFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var seg in segments)
        {
            current = current.Length == 0 ? seg : current + "/" + seg;
            if (_knownFolders.Contains(current)) continue;
            using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), Combine(current, true));
            using var resp = await _http.SendAsync(req, ct);
            // 201 créé ; 405 existe déjà ; 301/302 certains serveurs redirigent vers l'URL avec « / »
            if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.OK
                or HttpStatusCode.NoContent)
            {
                _knownFolders.Add(current);
                continue;
            }
            throw new WebDavException(Describe(resp, $"Création du dossier « {current} »"), resp.StatusCode);
        }
    }

    /// <summary>Dépose le fichier avec nouvelles tentatives sur erreur réseau ou serveur (5xx).</summary>
    public async Task<Uri> UploadAsync(string relativeFolder, string fileName, byte[] content, CancellationToken ct)
    {
        int attempts = Math.Max(1, _target.Retries + 1);
        Exception? last = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await EnsureFolderAsync(relativeFolder, ct);
                var uri = Combine(string.IsNullOrEmpty(relativeFolder) ? fileName : $"{relativeFolder}/{fileName}", false);

                using (var put = new HttpRequestMessage(HttpMethod.Put, uri))
                {
                    put.Content = new ByteArrayContent(content);
                    put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                    using var resp = await _http.SendAsync(put, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var ex = new WebDavException(Describe(resp, $"Dépôt de « {fileName} »"), resp.StatusCode);
                        if ((int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.RequestTimeout)
                            throw ex; // erreur définitive (droits, chemin) : inutile de réessayer
                        last = ex;
                        await Backoff(attempt, ct);
                        continue;
                    }
                }

                if (_target.VerifyAfterUpload) await VerifyAsync(uri, content.LongLength, ct);
                return uri;
            }
            catch (WebDavException ex) when (ex.StatusCode is null || (int)ex.StatusCode >= 500)
            {
                last = ex;
            }
            catch (HttpRequestException ex) { last = ex; }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; } // délai dépassé
            if (attempt < attempts) await Backoff(attempt, ct);
        }
        throw last as WebDavException ?? new WebDavException($"Échec du dépôt de « {fileName} » : {last?.Message}", null, last);
    }

    private async Task VerifyAsync(Uri uri, long expectedLength, CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, uri);
        using var resp = await _http.SendAsync(head, ct);
        if (resp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented) return; // HEAD non géré
        if (!resp.IsSuccessStatusCode)
            throw new WebDavException(Describe(resp, "Vérification après dépôt"), resp.StatusCode);
        var len = resp.Content.Headers.ContentLength;
        if (len.HasValue && len.Value != expectedLength)
            throw new WebDavException($"Vérification après dépôt : taille distante {len} ≠ {expectedLength} octets.");
    }

    private static Task Backoff(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt - 1))), ct);

    private static string Describe(HttpResponseMessage resp, string action) =>
        $"{action} : HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}" + ((int)resp.StatusCode switch
        {
            401 => " (identifiants refusés)",
            403 => " (droits insuffisants sur le dossier)",
            404 => " (URL WebDAV introuvable)",
            409 => " (dossier parent absent)",
            507 => " (espace disque insuffisant sur le serveur)",
            _ => ""
        });

    public void Dispose() => _http.Dispose();
}
