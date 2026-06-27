namespace BiblioText.Contracts;

/// <summary>
/// Versioning + well-known constants for the station→cloud publish contract.
/// Bump <see cref="SchemaVersion"/> whenever the shape of <see cref="PublishBatch"/>
/// or <see cref="BookPayload"/> changes in a non-additive way so the cloud can
/// reject or migrate older stations.
/// </summary>
public static class PublishContract
{
    /// <summary>Current wire schema version. Bump on breaking payload changes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Route the station POSTs a <see cref="PublishBatch"/> to.</summary>
    public const string PublishRoute = "/api/publish";
}
