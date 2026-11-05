FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/DocQa/DocQa.csproj src/DocQa/
RUN dotnet restore src/DocQa/DocQa.csproj
COPY src/DocQa/ src/DocQa/
RUN dotnet publish src/DocQa/DocQa.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# pdftotext is used to read PDF files
RUN apt-get update && apt-get install -y --no-install-recommends poppler-utils \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DocQa.dll"]
