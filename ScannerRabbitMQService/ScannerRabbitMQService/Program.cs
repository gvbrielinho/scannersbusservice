using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScannerRabbitMQService;
using Serilog;
using Serilog.Events;

public class Program
{
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateBootstrapLogger();

        try
        {
            Log.Information("Iniciando aplicación");

            var builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddHostedService<RabbitMQWorker>();
            builder.Services.AddHealthChecks();

            // Configurar Serilog
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff K} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .Enrich.WithProperty("TimeZone", TimeZoneInfo.Local.Id)
                .CreateBootstrapLogger());

            var app = builder.Build();
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "La aplicación falló al iniciar");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}