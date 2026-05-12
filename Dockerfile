FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env

ARG NUGET_USERNAME
ARG NUGET_PASSWORD

WORKDIR /app

COPY *.slnx .
COPY src/ ./src/
COPY test/ ./test/

RUN dotnet nuget add source "https://nuget.pkg.github.com/CodicePlastico/index.json" --store-password-in-clear-text -u $NUGET_USERNAME -p $NUGET_PASSWORD -n github
RUN dotnet restore
RUN dotnet publish src/DotNetTemplate.Host/DotNetTemplate.Host.csproj -c Release -o ./out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build-env /app/out ./DotNetTemplate.Host
ENTRYPOINT ["dotnet", "DotNetTemplate.Host/DotNetTemplate.Host.dll"]    