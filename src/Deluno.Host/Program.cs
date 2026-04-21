using Deluno.Api;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Integrations;
using Deluno.Jobs;
using Deluno.Movies;
using Deluno.Platform;
using Deluno.Realtime;
using Deluno.Series;
using Deluno.Worker;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

builder.Services.AddDelunoInfrastructure(builder.Configuration);
builder.Services.AddDelunoApi();
builder.Services.AddDelunoPlatformModule();
builder.Services.AddDelunoMoviesModule();
builder.Services.AddDelunoSeriesModule();
builder.Services.AddDelunoJobsModule();
builder.Services.AddDelunoIntegrationsModule();
builder.Services.AddDelunoFilesystemModule();
builder.Services.AddDelunoRealtimeModule();
builder.Services.AddDelunoWorkerModule();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapDelunoApi();
app.MapDelunoMoviesEndpoints();
app.MapDelunoSeriesEndpoints();
app.MapDelunoRealtime();
app.MapFallbackToFile("index.html");

app.Run();
