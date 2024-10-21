FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ScannerRabbitMQService/ScannerRabbitMQService.csproj", "ScannerRabbitMQService/"]
RUN dotnet restore "./ScannerRabbitMQService/ScannerRabbitMQService.csproj"
COPY . .
WORKDIR "/src/ScannerRabbitMQService"
RUN dotnet build "./ScannerRabbitMQService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ScannerRabbitMQService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

USER root

RUN apt-get update && apt-get install -y tzdata
ENV TZ=America/Argentina/Buenos_Aires

# Agregar variables de entorno para la configuración de RabbitMQ
ENV RABBITMQ_HOST=host.docker.internal \
    RABBITMQ_PORT=5672 \
    RABBITMQ_USER=guest \
    RABBITMQ_PASSWORD=guest \
    RABBITMQ_VHOST=/ \
    RABBITMQ_SCANNER_QUEUE=scanner_data \
    RABBITMQ_RESPONSE_QUEUE=scanner_response

# Exponer el puerto 8080 para el healthcheck
EXPOSE 8080

# Agregar un healthcheck
HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "ScannerRabbitMQService.dll"]