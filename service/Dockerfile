# Build the application using the .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY service.csproj .
# Cache dependency restoration until the project file changes.
RUN dotnet restore

COPY . .
RUN dotnet publish service.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

# Run using the smaller ASP.NET runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build --chown=app:app /app/publish .

# The non-root process needs private writable directories for keys and development mail.
RUN mkdir -p /app/.keys /app/.local-mail \
    && chown app:app /app/.keys /app/.local-mail \
    && chmod 700 /app/.keys /app/.local-mail

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "service.dll"]
