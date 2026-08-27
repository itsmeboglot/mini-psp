FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first, against the project file alone, so that a source-only change
# does not invalidate the restore layer.
COPY src/Payments.Api/Payments.Api.csproj src/Payments.Api/
RUN dotnet restore src/Payments.Api/Payments.Api.csproj

COPY src/ src/
RUN dotnet publish src/Payments.Api/Payments.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The base image ships a non-root 'app' user; nothing here needs more.
USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "Payments.Api.dll"]
