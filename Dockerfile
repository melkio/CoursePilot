# syntax=docker/dockerfile:1.10
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /app
COPY *.slnx ./
COPY src/ ./src/
COPY test/ ./test/
RUN dotnet nuget add source https://nuget.pkg.github.com/CodicePlastico/index.json --name codiceplastico
RUN --mount=type=secret,id=nuget_credentials,env=NuGetPackageSourceCredentials_codiceplastico,required=true \
    dotnet restore CoursePilot.slnx && \
    dotnet publish src/CoursePilot.Host/CoursePilot.Host.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build-env /app/out ./CoursePilot.Host
ENTRYPOINT ["dotnet", "CoursePilot.Host/CoursePilot.Host.dll"]
