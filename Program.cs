using EndpointSignalAgent;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

// Enables running as a Windows Service when installed,
// but still runs fine as console when debugging.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EndpointSignalAgent";
});

// HttpClient for sending data to backend
//builder.Services.AddHttpClient("backend", client =>
//{
//    // Overridden by appsettings.json; keep safe default
//    client.Timeout = TimeSpan.FromSeconds(10);
//});

builder.Services.AddHttpClient("backend", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(cfg["Backend:BaseUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(10);
});


builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
