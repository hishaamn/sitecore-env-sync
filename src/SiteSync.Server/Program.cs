using System.Text.Json.Serialization;
using SiteSync.Server.Connectors;
using SiteSync.Server.Domain;
using SiteSync.Server.Hubs;
using SiteSync.Server.Persistence;
using SiteSync.Server.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddHttpClient();
builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<IConnectorFactory, ConnectorFactory>();
builder.Services.AddSingleton<IProgressPublisher, SignalRProgressPublisher>();
builder.Services.AddSingleton<JobService>();

// Vite dev server runs on a different port; SignalR needs credentials allowed.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHub<SyncHub>("/hubs/sync");
app.MapFallbackToFile("index.html");

// First run: seed two simulated environments so the dashboard is usable immediately.
using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<FileStore>();
    if (store.GetEnvironments().Count == 0)
    {
        store.SaveEnvironments(new List<SitecoreEnvironment>
        {
            new()
            {
                Name = "DEV (simulated)",
                BaseUrl = "https://dev.sitecore.local",
                Username = "admin",
                Domain = "sitecore",
                Databases = new() { "master", "web" },
                ConnectorType = ConnectorType.Simulated,
                Color = "#22c55e"
            },
            new()
            {
                Name = "UAT (simulated)",
                BaseUrl = "https://uat.sitecore.local",
                Username = "admin",
                Domain = "sitecore",
                Databases = new() { "master", "web" },
                ConnectorType = ConnectorType.Simulated,
                Color = "#f59e0b"
            }
        });
    }
}

app.Run();
