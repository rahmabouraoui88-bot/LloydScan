using LloydScan.Core.Configuration;

namespace LloydScan.Core.Upload;

public sealed record WebDavCredential(string Username, string Password);

/// <summary>Fournit le compte WebDAV à utiliser pour une destination (compte de l'agent ou compte de service).</summary>
public interface ICredentialProvider
{
    /// <summary>null si aucun identifiant n'est disponible (ou en authentification Windows).</summary>
    WebDavCredential? Get(string targetName, WebDavTarget target);
}
