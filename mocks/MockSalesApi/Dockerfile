FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY mocks/MockSalesApi/MockSalesApi.csproj mocks/MockSalesApi/
RUN dotnet restore mocks/MockSalesApi/MockSalesApi.csproj
COPY mocks/MockSalesApi/ mocks/MockSalesApi/
RUN dotnet publish mocks/MockSalesApi/MockSalesApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MockSalesApi.dll"]
