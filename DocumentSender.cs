using System.Text;
using System.Text.Json;
using LloydScan.Core.Configuration;
using LloydScan.Core.Processing;

namespace LloydScan.Core.Upload;

/// <summary>Élément en file d'attente locale (dépôt à rejouer).</summary>
public sealed class OutboxItem
{
    public string Target { get; set; } = "";
    public string Folder { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? QrText { get; set; }
    public string BatchId { get; set; } = "";
    public int Pages { get; set; }
    public long InputBytes { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
}

/// <summary>
/// Envoi des documents vers la bonne instance WebDAV.
/// En cas d'échec, le PDF est mis en file d'attente locale : aucun document n'est perdu.
/// Chaque envoi est tracé dans un journal CSV mensuel (piste d'audit).
/// </summary>
public sealed class DocumentSender : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ICredentialProvider _credentials;
    private readonly Dictionary<string, (WebDavClient client, string account)> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _work;
    private static readonly SemaphoreSlim JournalLock = new(1, 1);

    /// <param name="credentials">Comptes WebDAV de l'agent (ou de service) par destination.</param>
    public DocumentSender(AppSettings settings, ICredentialProvider credentials)
    {
        _settings = settings;
        _credentials = credentials;
        _work = settings.Storage.ResolveWorkFolder();
    }

    public string OutboxFolder => Path.Combine(_work, "Outbox");
    public string JournalFolder => Path.Combine(_work, "Journal");

    private (WebDavClient client, string account) Client(string target)
    {
        if (_clients.TryGetValue(target, out var c)) return c;
        if (!_settings.Targets.TryGetValue(target, out var t))
            throw new InvalidOperationException($"Destination WebDAV inconnue : {target}");
        var credential = _credentials.Get(target, t);
        c = (new WebDavClient(t, credential), AccountLabel(t, credential));
        _clients[target] = c;
        return c;
    }

    private static string AccountLabel(WebDavTarget t, WebDavCredential? credential) =>
        t.Authentication == WebDavAuthentication.Windows
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : credential?.Username ?? "";

    /// <summary>À appeler après une modification des identifiants de l'agent.</summary>
    public void ResetClients()
    {
        foreach (var c in _clients.Values) c.client.Dispose();
        _clients.Clear();
    }

    /// <summary>Teste un compte avant de l'enregistrer (fenêtre « Mes identifiants »).</summary>
    public static async Task TestAsync(WebDavTarget target, WebDavCredential? credential, CancellationToken ct)
    {
        using var client = new WebDavClient(target, credential);
        await client.TestConnectionAsync(ct);
    }

    public string RemoteFolder(string target, DateTimeOffset date, string batchId, string? qr = null)
    {
        var t = _settings.Targets[target];
        var values = new Dictionary<string, object?>
        {
            ["date"] = date, ["target"] = target, ["batch"] = batchId,
            ["qr"] = TemplateFormatter.SafeSegment(qr, _settings.Naming.MaxQrLength, "SANS-QR"),
            ["user"] = Environment.UserName, ["machine"] = Environment.MachineName
        };
        return TemplateFormatter.SafePath(TemplateFormatter.Format(t.FolderPattern, values));
    }

    public async Task SendAsync(ScanDocument doc, BatchResult batch, CancellationToken ct)
    {
        if (doc.Pdf is null) throw new InvalidOperationException("Le PDF n'est pas généré.");
        var folder = RemoteFolder(doc.Target, batch.StartedAt, batch.BatchId, doc.QrText);
        doc.Status = DocumentStatus.Sending;
        try
        {
            var (client, account) = Client(doc.Target);
            doc.Account = account;
            var uri = await client.UploadAsync(folder, doc.FileName, doc.Pdf, ct);
            doc.RemoteUrl = uri.ToString();
            doc.Status = DocumentStatus.Sent;
            doc.Error = null;
            if (_settings.Storage.KeepLocalCopy) await SaveCopyAsync("Archive", doc.Target, doc.FileName, doc.Pdf, ct);
            await JournalAsync(batch.BatchId, doc.Target, doc.Account, doc.FileName, doc.QrText, doc.PageCount, doc.InputBytes, doc.PdfBytes, "ENVOYE",
                doc.Edits.Count == 0 ? doc.RemoteUrl : $"{doc.RemoteUrl} | retouches : {string.Join(", ", doc.Edits)}");
            ScanPipeline.DeleteSources(doc.Pages); // copies de travail inutiles une fois le PDF déposé
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            doc.Status = DocumentStatus.Ready;
            throw;
        }
        catch (Exception ex)
        {
            doc.Error = ex.Message;
            await EnqueueAsync(doc, batch, folder, ex.Message);
            doc.Status = DocumentStatus.Queued;
            ScanPipeline.DeleteSources(doc.Pages); // le PDF est conservé dans la file d'attente
            await JournalAsync(batch.BatchId, doc.Target, doc.Account, doc.FileName, doc.QrText, doc.PageCount, doc.InputBytes, doc.PdfBytes, "EN_ATTENTE", ex.Message);
        }
    }

    private async Task EnqueueAsync(ScanDocument doc, BatchResult batch, string folder, string error)
    {
        var dir = Path.Combine(OutboxFolder, doc.Target);
        Directory.CreateDirectory(dir);
        var pdfPath = Path.Combine(dir, doc.FileName);
        await File.WriteAllBytesAsync(pdfPath, doc.Pdf!);
        var item = new OutboxItem
        {
            Target = doc.Target, Folder = folder, FileName = doc.FileName, QrText = doc.QrText,
            BatchId = batch.BatchId, Pages = doc.PageCount, InputBytes = doc.InputBytes, Attempts = 1, LastError = error,
            QueuedAt = DateTimeOffset.Now
        };
        await File.WriteAllTextAsync(pdfPath + ".json", JsonSerializer.Serialize(item, AppSettings.JsonOptions));
    }

    public int PendingCount() =>
        Directory.Exists(OutboxFolder) ? Directory.GetFiles(OutboxFolder, "*.pdf.json", SearchOption.AllDirectories).Length : 0;

    /// <summary>Rejoue les dépôts en attente. Renvoie (envoyés, toujours en attente).</summary>
    public async Task<(int sent, int pending)> RetryOutboxAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(OutboxFolder)) return (0, 0);
        int sent = 0, pending = 0;
        foreach (var meta in Directory.GetFiles(OutboxFolder, "*.pdf.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var item = JsonSerializer.Deserialize<OutboxItem>(await File.ReadAllTextAsync(meta, ct), AppSettings.JsonOptions);
            var pdfPath = meta[..^".json".Length];
            if (item is null || !File.Exists(pdfPath)) continue;
            try
            {
                progress?.Report($"Renvoi de {item.FileName}…");
                var bytes = await File.ReadAllBytesAsync(pdfPath, ct);
                var (client, account) = Client(item.Target);
                var uri = await client.UploadAsync(item.Folder, item.FileName, bytes, ct);
                File.Delete(pdfPath);
                File.Delete(meta);
                sent++;
                await JournalAsync(item.BatchId, item.Target, account, item.FileName, item.QrText, item.Pages, item.InputBytes, bytes.LongLength, "ENVOYE_REPRISE", uri.ToString());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Attempts++;
                item.LastError = ex.Message;
                await File.WriteAllTextAsync(meta, JsonSerializer.Serialize(item, AppSettings.JsonOptions), ct);
                pending++;
            }
        }
        return (sent, pending);
    }

    private async Task SaveCopyAsync(string kind, string target, string fileName, byte[] pdf, CancellationToken ct)
    {
        var dir = Path.Combine(_work, kind, target, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), pdf, ct);
    }

    /// <summary>Journal CSV (séparateur « ; », UTF-8 avec BOM : s'ouvre directement dans Excel).</summary>
    public async Task JournalAsync(string batchId, string target, string? account, string fileName, string? qr, int pages,
        long inputBytes, long bytes, string status, string? detail)
    {
        Directory.CreateDirectory(JournalFolder);
        var path = Path.Combine(JournalFolder, $"journal-{DateTime.Now:yyyy-MM}.csv");
        static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var line = string.Join(';', Csv(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")), Csv(Environment.MachineName),
            Csv($"{Environment.UserDomainName}\\{Environment.UserName}"), Csv(batchId), Csv(target), Csv(account), Csv(fileName), Csv(qr), pages.ToString(),
            inputBytes.ToString(), bytes.ToString(), Csv(status), Csv(detail));
        await JournalLock.WaitAsync();
        try
        {
            bool isNew = !File.Exists(path);
            await using var sw = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (isNew) await sw.WriteLineAsync("Date;Poste;Agent;Lot;Destination;CompteWebDAV;Fichier;QR;Pages;OctetsAvant;OctetsApres;Statut;Detail");
            await sw.WriteLineAsync(line);
        }
        finally { JournalLock.Release(); }
    }

    public void Dispose()
    {
        ResetClients();
    }
}
