# Waste Obligations

An API for managing obligation data in relation to EPR.

## Prerequisites

- .NET 10
- Docker

## Setup process

- Ensure .NET 10 SDK is installed
- Ensure a container runtime is installed

### Running locally via Docker

```bash
docker compose up -d --wait
```

### Running via .NET

Mongo will be needed and can be started as follows:

```bash
docker compose up mongodb -d --wait
```

Start the API as follows:

```bash
dotnet run --project ./src/Api --launch-profile Api
```

The same port (8080) is used for launch profile and Docker compose configuration, therefore only one can run at any one time. 

### Documentation

API documentation can be viewed at http://localhost:8080/documentation/index.html once the service is running.

Additional project documentation:

- [Analytics compliance declaration events](docs/analytics-compliance-declaration-events.md)
- [Analytics obligation calculation events](docs/analytics-obligation-calculation-events.md)

## Concepts

### Compliance declaration analytics events

The [analytics event guide](docs/analytics-compliance-declaration-events.md) describes the published contract and schema history. The [event flow](docs/compliance-declaration-end-to-end-event-flow.md) explains how transactional capture relates to asynchronous publication.

### Stopping and clearing local resources

```bash
docker compose down
```

To remove local data:

```bash
docker compose down -v --remove-orphans
```

## Tests

Tests with the `IntegrationTests` trait require additional local dependencies - either the API running in Docker or Mongo.

Running unit tests:

```bash
dotnet build tests/Api.Tests/Api.Tests.csproj
dotnet test --test-modules tests/Api.Tests/bin/Debug/net10.0/Api.Tests.dll --no-build
```

Running integration tests with dependencies:

```bash
dotnet build tests/Api.IntegrationTests/Api.IntegrationTests.csproj
dotnet test --test-modules tests/Api.IntegrationTests/bin/Debug/net10.0/Api.IntegrationTests.dll --no-build
```

Running all tests (with the integration-test dependencies available):

```bash
dotnet build
dotnet test --test-modules tests/Api.Tests/bin/Debug/net10.0/Api.Tests.dll --no-build
dotnet test --test-modules tests/Api.IntegrationTests/bin/Debug/net10.0/Api.IntegrationTests.dll --no-build
```

### Govuk Notify

The nullable [email delivery cutover](docs/email-delivery-cutover.md) defaults to null, preserving current email behaviour. Configure the shared future UTC boundary only when the replacement Notifications producers are ready.

The `GovukNotifyTests` integration tests can run against Govuk Notify if you provide an API Key.

Set the `GOVUKNOTIFY_APIKEY` env var in a terminal and then run the integration tests:

```
export GOVUKNOTIFY_APIKEY=[replace with key]
dotnet build tests/Api.IntegrationTests/Api.IntegrationTests.csproj
dotnet test --test-modules tests/Api.IntegrationTests/bin/Debug/net10.0/Api.IntegrationTests.dll --no-build
```

The test runner writes console output so use of the API Key can be seen.


## Code quality

SonarQube cloud is configured and all Defra rules are mandated. 

See https://sonarcloud.io/project/overview?id=DEFRA_waste-obligations for project information.

## Dependency management

Dependabot is configured for ongoing dependency management.

See [dependabot.yml](.github/dependabot.yml) for group configuration.

## Build pipeline

- [Pull requests](.github/workflows/check-pull-request.yml)
  - Run all tests
  - Build Docker image
  - Check image with Trivy
  - Sonar
  - Run Waste Obligations journeys for trusted pull requests
- [Publish](.github/workflows/publish.yml)
  - Merge PR to main
  - Build Docker image and publish to CDP
  - Sonar

## Scheduled jobs

Scheduled-job registration and execution are implemented in [the scheduling services](src/Api/Services/Scheduling) and configured in [appsettings.json](src/Api/appsettings.json). The [obligation calculation job](src/Api/ScheduledJobs/ObligationCalculationJob.cs) creates instructions; the [processor](src/Api/ScheduledJobs/ObligationCalculationProcessor.cs) fetches the preceding packaging year and saves calculations for each selected submitter. Scheduling and processing are independently disabled by default. Before enabling processing, configure `WastePackaging__BaseAddress`, `WastePackaging__ClientId` and `WastePackaging__ClientSecret` with the endpoint and matching read-scoped credentials.

Apply migrations before enabling either producer or processor. Migration 021's partial indexes require MongoDB 6.0 or later; confirm the deployed version first. See [migration 021](src/Api/Data/Migrations/021_ObligationCalculationInstructionIndexes.cs) and the [instruction integration tests](tests/Api.IntegrationTests/Services/ObligationCalculations). Lease expiry permits recovery after a crash but does not prevent overlap during a host pause or network partition. The [calculation service](src/Api/Services/ObligationCalculations/ObligationCalculationService.cs) leaves unchanged results untouched when they are saved again.

Set configuration on the API container in the target environment; local Compose settings do not configure CDP. A future queue message should refer to an existing instruction rather than create another one. Reliable queue publication and acknowledgement require separate implementation.

Each scheduled handoff creates the instruction and records the occurrence in one MongoDB transaction. Hosts that observed an occurrence retry a failed handoff until the next occurrence is due. If every host that observed it stops before the handoff commits, the occurrence is not replayed. The MongoDB deployment must support transactions, including creation of the scheduler state collection during the first handoff. The transaction uses the configured `Mongo:TransactionTimeoutSeconds` budget (five seconds by default). Scheduled jobs must use the supplied session for durable handoff; external calls and other nontransactional effects belong in the processing worker after commit.

## System health checks

The [.NET health-check CLI](tools/healthcheck/README.md) checks configurable services and their dependencies. Configure service addresses in `tools/healthcheck/appsettings.json` and credentials in the Git-ignored `tools/healthcheck/.env` file.

## CDP

Review CDP documentation and process for relevant portal operations.

## Journey tests

For repository-secret setup, see [Journey tests in Confluence](https://eaflood.atlassian.net/wiki/spaces/EDIA/pages/6597640835/Journey+tests).

Trusted pull requests and manually dispatched **Check Pull Request** runs execute the Waste Obligations journey suite in a separate job. The job builds the selected source revision locally; it does not publish a test image. If a branch with the same name exists in `waste-obligations-journey-tests`, that branch supplies the journeys and dedicated CI stack; otherwise the suite uses `main`.

The required GitHub Actions secrets and OIDC/CDP-role prerequisite are documented in the [journey-test CI contract](https://github.com/DEFRA/waste-obligations-journey-tests#github-actions-secrets). Configure that shared contract in this repository, or use organisation-level secrets restricted to the journey and service repositories. Fork pull requests do not run the job because repository secrets are unavailable to them.

## Github

The following secrets are configured in the repository in Github:

GOVUKNOTIFY_APIKEY - the correct GOV.UK Notify Test API Key used by the API integration tests. Ensure it remains a Test API Key as per guidance https://docs.notifications.service.gov.uk/java.html#test.

GOVUK_NOTIFY_API_KEY - a separate, format-valid dummy key used only by the journey-test stack's WireMock-backed GOV.UK Notify mapping. It must not replace `GOVUKNOTIFY_APIKEY` and does not call GOV.UK Notify. Its shared setup is documented in the [journey-test CI contract](https://github.com/DEFRA/waste-obligations-journey-tests#github-actions-secrets).

Also ensure it's defined in the main repository secrets and also the Dependabot secrets.

SONAR_TOKEN - the API Key for SonarCloud https://sonarcloud.io/project/overview?id=DEFRA_waste-obligations

## Licence Information

THIS INFORMATION IS LICENSED UNDER THE CONDITIONS OF THE OPEN GOVERNMENT LICENCE found at:

http://www.nationalarchives.gov.uk/doc/open-government-licence/version/3

### About the licence

The Open Government Licence (OGL) was developed by the Controller of Her Majesty's Stationery Office (HMSO) to enable information providers in the public sector to license the use and re-use of their information under a common open licence.

It is designed to encourage use and re-use of information freely and flexibly, with only a few conditions.
