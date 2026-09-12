using SiteSync.Server.Domain;

namespace SiteSync.Server.Connectors;

public class ConnectorFactory : IConnectorFactory
{
    private readonly IHttpClientFactory _httpFactory;

    public ConnectorFactory(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    public ISitecoreConnector Create(SitecoreEnvironment environment) => environment.ConnectorType switch
    {
        ConnectorType.Simulated => new SimulatedConnector(environment),
        _ => new ItemServiceConnector(environment, _httpFactory)
    };
}
