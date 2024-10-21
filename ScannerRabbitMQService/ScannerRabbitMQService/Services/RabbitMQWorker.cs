using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Polly;
using System.Net.Sockets;
using System.Net;
using Serilog;
using ILogger = Serilog.ILogger;

namespace ScannerRabbitMQService
{
    public class RabbitMQWorker : BackgroundService
    {
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private IConnection? _connection;
        private IModel? _channel;
        private readonly HttpListener _listener;

        private string ScannerQueue => _configuration["RabbitMQ:ScannerQueue"] ?? throw new InvalidOperationException("ScannerQueue configuration is missing");
        private string PackageQueue => _configuration["RabbitMQ:PackageQueue"] ?? throw new InvalidOperationException("PackageQueue configuration is missing");

        private int MessagesReceived = 0;

        public RabbitMQWorker(IConfiguration configuration)
        {
            _logger = Log.ForContext<RabbitMQWorker>();
            _configuration = configuration;
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://+:8080/");
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.Information("RabbitMQ Worker iniciando...");
            InitializeRabbitMQ();
            StartHealthCheckServer();
            return base.StartAsync(cancellationToken);
        }

        private void StartHealthCheckServer()
        {
            _listener.Start();
            Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        if (context.Request.Url?.PathAndQuery == "/health")
                        {
                            string responseString = "Healthy";
                            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                            context.Response.ContentLength64 = buffer.Length;
                            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                            context.Response.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error en el servidor de health check");
                    }
                }
            });
        }

        private void InitializeRabbitMQ()
        {
            var policy = Policy
                .Handle<BrokerUnreachableException>()
                .Or<SocketException>()
                .WaitAndRetry(5, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    (ex, time) =>
                    {
                        _logger.Warning(ex, "No se pudo conectar a RabbitMQ. Reintentando en {TimeOut}s", $"{time.TotalSeconds:n1}");
                    }
                );

            policy.Execute(() =>
            {
                var factory = new ConnectionFactory
                {
                    HostName = _configuration["RABBITMQ_HOST"] ?? "rabbitmq",
                    UserName = _configuration["RabbitMQ:UserName"],
                    Password = _configuration["RabbitMQ:Password"],
                    VirtualHost = _configuration["RabbitMQ:VirtualHost"]
                };

                if (int.TryParse(_configuration["RabbitMQ:Port"], out int port))
                {
                    factory.Port = port;
                }
                else
                {
                    _logger.Warning("Puerto RabbitMQ no válido en la configuración. Usando el puerto predeterminado 5672.");
                    factory.Port = 5672;
                }

                _logger.Information("Intentando conectar a RabbitMQ con los siguientes parámetros: Host={Host}, Port={Port}, User={User}, VHost={VHost}",
                    factory.HostName, factory.Port, factory.UserName, factory.VirtualHost);

                _connection = factory.CreateConnection();
                _logger.Information("Conexión a RabbitMQ establecida exitosamente");

                _channel = _connection.CreateModel();
                _logger.Information("Canal RabbitMQ creado exitosamente");

                _channel.QueueDeclare(queue: ScannerQueue, durable: true, exclusive: false, autoDelete: false, arguments: null);
                _channel.QueueDeclare(queue: PackageQueue, durable: true, exclusive: false, autoDelete: false, arguments: null);
                _logger.Information("Colas RabbitMQ declaradas: ScannerQueue={ScannerQueue}, PackageQueue={PackageQueue}", ScannerQueue, PackageQueue);

                _connection.ConnectionShutdown += RabbitMQ_ConnectionShutdown!;
                _logger.Information("Evento ConnectionShutdown registrado");
            });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.Information("RabbitMQ Worker ejecutándose a las: {Time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            stoppingToken.Register(() => _logger.Information("RabbitMQ Worker está parando..."));

            ConsumeMessages();

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }

        private void ConsumeMessages()
        {
            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                _logger.Information("Mensaje recibido de la cola {QueueName}: {message}", ea.RoutingKey, message);

                MessagesReceived++;
                _logger.Information("Total de mensajes recibidos: {count}", MessagesReceived);

                ProcessMessage(ea.RoutingKey, message);

                _channel!.BasicAck(ea.DeliveryTag, multiple: false);
            };

            _channel.BasicConsume(queue: ScannerQueue, autoAck: false, consumer: consumer);
            _channel.BasicConsume(queue: PackageQueue, autoAck: false, consumer: consumer);
        }

        private void ProcessMessage(string queueName, string message)
        {
            try
            {
                var messageObject = JsonConvert.DeserializeObject<dynamic>(message);

                if (queueName == ScannerQueue)
                {
                    ProcessScannerMessage(messageObject);
                }
                else if (queueName == PackageQueue)
                {
                    ProcessPackageMessage(messageObject);
                }
                else
                {
                    _logger.Warning("Mensaje recibido de una cola desconocida: {QueueName}", queueName);
                }
            }
            catch (JsonException ex)
            {
                _logger.Error(ex, "Error al deserializar el mensaje: {Message}", message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error al procesar el mensaje: {Message}", message);
            }
        }

        private void ProcessScannerMessage(dynamic message)
        {
            _logger.Information("Procesando mensaje del escáner: {Message}", JsonConvert.SerializeObject(message));
            // Implementa aquí la lógica específica para los mensajes del escáner
        }

        private void ProcessPackageMessage(dynamic message)
        {
            _logger.Information("Procesando mensaje del paquete: {Message}", JsonConvert.SerializeObject(message));
            // Implementa aquí la lógica específica para los mensajes de paquetes
        }

        private void RabbitMQ_ConnectionShutdown(object sender, ShutdownEventArgs e)
        {
            _logger.Warning("Conexión RabbitMQ cerrada. Intentando reconectar...");
            InitializeRabbitMQ();
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.Information("RabbitMQ Worker detenido a las: {time}", DateTimeOffset.Now);

            try
            {
                _channel?.Close();
                _connection?.Close();
                _listener.Stop();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error al cerrar la conexión RabbitMQ o el servidor de health check");
            }

            await base.StopAsync(cancellationToken);
        }
    }
}