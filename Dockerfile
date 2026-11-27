# Plain Dockerfile with no BuildKit-only features, so it builds with Synology Container Manager's
# builder directly on the NAS. To build on a PC for the NAS instead, use build-for-synology.ps1.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/BlackVueSync/BlackVueSync.csproj .
RUN dotnet restore
COPY src/BlackVueSync/ .
RUN dotnet publish -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENV BlackVue__Destination=/recordings
VOLUME /recordings
ENTRYPOINT ["dotnet", "BlackVueSync.dll"]
