FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Harness.sln --locked-mode
RUN dotnet publish src/Harness.Host/Harness.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Harness.Host.dll"]
