FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/Payments.Core/Payments.Core.csproj src/Payments.Core/
COPY src/Payments.Worker/Payments.Worker.csproj src/Payments.Worker/
RUN dotnet restore src/Payments.Worker/Payments.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Payments.Worker/Payments.Worker.csproj -c Release -o /app --no-restore

# aspnet rather than runtime: the worker exposes a metrics endpoint.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Payments.Worker.dll"]
