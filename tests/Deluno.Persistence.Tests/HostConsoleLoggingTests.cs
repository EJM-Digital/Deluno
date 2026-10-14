using System.IO.Pipes;
using Deluno.Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests;

/// <summary>
/// #1258: the headless and Docker host keeps its console logger. If whatever reads its standard output stops reading,
/// the pipe fills; the host must go on working, and the lines it cannot write are dropped (the log file keeps them all).
/// </summary>
[Collection("The process's console")]
public sealed class HostConsoleLoggingTests
{
    [Fact]
    public async Task A_host_whose_standard_output_is_never_read_goes_on_working_and_never_waits_on_it()
    {
        using var reader = new AnonymousPipeServerStream(PipeDirection.In);
        using var writer = new AnonymousPipeClientStream(PipeDirection.Out, reader.ClientSafePipeHandle);
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var text = new StreamWriter(writer) { AutoFlush = true };
        Console.SetOut(text);
        Console.SetError(text);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Services.MakeNonBlocking();
            await using var app = builder.Build();
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("host");
            var filler = new string('x', 2000);

            var logging = Task.Run(() =>
            {
                for (var i = 0; i < 3000; i++)
                {
                    logger.LogWarning("line {Number} {Filler}", i, filler);
                }
            });

            Assert.True(await Task.WhenAny(logging, Task.Delay(TimeSpan.FromSeconds(20))) == logging, "Logging waited on the unread standard output."); // reason: the 20 s is the ceiling that proves logging did not block on unread output, not a measured wait
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            reader.Dispose();
        }
    }
}
