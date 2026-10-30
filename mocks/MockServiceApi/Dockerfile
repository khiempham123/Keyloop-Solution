FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY mocks/MockServiceApi/MockServiceApi.csproj mocks/MockServiceApi/
RUN dotnet restore mocks/MockServiceApi/MockServiceApi.csproj
COPY mocks/MockServiceApi/ mocks/MockServiceApi/
RUN dotnet publish mocks/MockServiceApi/MockServiceApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MockServiceApi.dll"]
