# docker build -t campuspass .
# docker run -p 8080:8080 -v campuspass-data:/app/App_Data campuspass
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/CampusPass.Web/CampusPass.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/App_Data && chown app /app/App_Data
# Cloud hosts put a proxy in front of the app: trust its X-Forwarded-* headers (real client IP + https).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
EXPOSE 8080
VOLUME /app/App_Data
USER app
ENTRYPOINT ["dotnet", "CampusPass.Web.dll"]
