# Build context is the repository root, not this directory: central package management
# (Directory.Packages.props) and Directory.Build.props live there, and a restore without
# them resolves nothing.
#   docker build -f src/JobPlatform.Api/Dockerfile -t job-platform-api .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Project files first, so a change to source code does not invalidate the restore layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/JobPlatform.Core/JobPlatform.Core.csproj src/JobPlatform.Core/
COPY src/JobPlatform.Data/JobPlatform.Data.csproj src/JobPlatform.Data/
COPY src/JobPlatform.Ai/JobPlatform.Ai.csproj     src/JobPlatform.Ai/
COPY src/JobPlatform.Documents/JobPlatform.Documents.csproj src/JobPlatform.Documents/
COPY src/JobPlatform.Api/JobPlatform.Api.csproj   src/JobPlatform.Api/
RUN dotnet restore src/JobPlatform.Api/JobPlatform.Api.csproj

COPY src/ src/
RUN dotnet publish src/JobPlatform.Api/JobPlatform.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# The image is published to a public registry, so it runs as a non-root user and carries no
# configuration: every setting arrives as an environment variable from Container Apps, and
# every credential is a managed identity token acquired at runtime.
#
# The .NET base images already ship a non-root `app` user and expose its id as APP_UID.
# Creating one fails the build with useradd exit code 9, "username already in use", so the
# existing user is used rather than a hand-rolled one.
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0

ENTRYPOINT ["dotnet", "JobPlatform.Api.dll"]
