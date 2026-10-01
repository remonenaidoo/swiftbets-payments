# syntax=docker/dockerfile:1.7
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY .packages/ .packages/
COPY src/ src/
RUN dotnet restore src/SwiftBets.Payments.Api/SwiftBets.Payments.Api.csproj -a $TARGETARCH
RUN dotnet publish src/SwiftBets.Payments.Api/SwiftBets.Payments.Api.csproj -c Release -a $TARGETARCH --no-restore --self-contained false -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SwiftBets.Payments.Api.dll"]
