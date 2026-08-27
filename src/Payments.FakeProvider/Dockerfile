FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Payments.FakeProvider/Payments.FakeProvider.csproj src/Payments.FakeProvider/
RUN dotnet restore src/Payments.FakeProvider/Payments.FakeProvider.csproj
COPY src/Payments.FakeProvider/ src/Payments.FakeProvider/
RUN dotnet publish src/Payments.FakeProvider/Payments.FakeProvider.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Payments.FakeProvider.dll"]
