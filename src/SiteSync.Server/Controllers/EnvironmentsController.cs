using Microsoft.AspNetCore.Mvc;
using SiteSync.Server.Connectors;
using SiteSync.Server.Domain;
using SiteSync.Server.Persistence;

namespace SiteSync.Server.Controllers;

[ApiController]
[Route("api/environments")]
public class EnvironmentsController : ControllerBase
{
    private readonly FileStore _store;
    private readonly IConnectorFactory _connectors;

    public EnvironmentsController(FileStore store, IConnectorFactory connectors)
    {
        _store = store;
        _connectors = connectors;
    }

    /// <summary>Passwords are never returned to the client.</summary>
    private static object Sanitize(SitecoreEnvironment e) => new
    {
        e.Id, e.Name, e.BaseUrl, e.Username, e.Domain, e.Databases, e.ConnectorType, e.Color,
        HasPassword = !string.IsNullOrEmpty(e.Password)
    };

    [HttpGet]
    public IActionResult List() => Ok(_store.GetEnvironments().Select(Sanitize));

    [HttpPost]
    public IActionResult Create([FromBody] SitecoreEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(env.Name)) return BadRequest(new { error = "Name is required." });
        if (env.ConnectorType == ConnectorType.ItemService && string.IsNullOrWhiteSpace(env.BaseUrl))
            return BadRequest(new { error = "Base URL is required for Item Service environments." });
        if (env.Databases.Count == 0) env.Databases = new() { "master", "web" };

        var envs = _store.GetEnvironments();
        env.Id = Guid.NewGuid().ToString("N");
        envs.Add(env);
        _store.SaveEnvironments(envs);
        return Ok(Sanitize(env));
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] SitecoreEnvironment update)
    {
        var envs = _store.GetEnvironments();
        var existing = envs.FirstOrDefault(e => e.Id == id);
        if (existing == null) return NotFound();

        existing.Name = update.Name;
        existing.BaseUrl = update.BaseUrl;
        existing.Username = update.Username;
        existing.Domain = update.Domain;
        existing.Databases = update.Databases.Count > 0 ? update.Databases : existing.Databases;
        existing.ConnectorType = update.ConnectorType;
        existing.Color = update.Color;
        if (!string.IsNullOrEmpty(update.Password)) existing.Password = update.Password; // blank = keep current

        _store.SaveEnvironments(envs);
        return Ok(Sanitize(existing));
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var envs = _store.GetEnvironments();
        var removed = envs.RemoveAll(e => e.Id == id);
        if (removed == 0) return NotFound();
        _store.SaveEnvironments(envs);
        return NoContent();
    }

    [HttpPost("{id}/test")]
    public async Task<IActionResult> Test(string id, CancellationToken ct)
    {
        var env = _store.GetEnvironments().FirstOrDefault(e => e.Id == id);
        if (env == null) return NotFound();
        var result = await _connectors.Create(env).TestConnectionAsync(ct);
        return Ok(result);
    }
}
