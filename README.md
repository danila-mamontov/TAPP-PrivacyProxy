# Getting Started
Privacy Proxy is in development and only running via Development Environment!

Therefore you have to adjust the `appsettings.Development.json` and not `appsettings.json` at the moment.

To set it up, you have to clone the project and adjust the configuration like in the example:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },

  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.AspNetCore": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console" }
    ]
  },
  
  "Presidio": {
    "AnalyzerUrl": "http://localhost:5002",
    "AllowList": [],
    "Context": [],
    "ScoreThreshold": 0.4,
    "GermanEntityTypes": ["PERSON", "LOCATION", "ORGANIZATION"],
    "EnglishEntityTypes": ["EMAIL_ADDRESS", "PHONE_NUMBER", "IP_ADDRESS", "CREDIT_CARD", "IBAN_CODE", "URL"],
    "GermanEntityThresholds": {
      "PERSON": 0.85,
      "LOCATION": 0.90,
      "ORGANIZATION": 0.85
    },
    "EnglishEntityThresholds": {
      "EMAIL_ADDRESS": 0.4,
      "PHONE_NUMBER": 0.4,
      "IP_ADDRESS": 0.4,
      "CREDIT_CARD": 0.4,
      "IBAN_CODE": 0.4,
      "URL": 0.4
    }
  },
  
  "Llm": {
    "BaseUrl": "http://localhost:11434/v1/",
    "ApiKey": "ollama"
  },
  
  "AllowedHosts": "*",
  
  "Urls": "http://*:6000;https://*:6001"
}

```

This section describes the local development setup without Docker.
For a containerized setup of PrivacyProxy and the Presidio Analyzer, see the [Docker](#docker) section below.

To run locally you have to use dotnet 10.

```bash
cd .../OpenClaw_PrivacyProxy/PrivacyProxy.Api
dotnet restore
dotnet run
```

To run tests you have to do:
```
cd .../OpenClaw_PrivacyProxy/PrivacyProxy.Api.Tests
dotnet restore
dotnet test
```

dotCover by JetBrains is fully supported. Riders IDE is suggested.

To use it between OpenClaw and LLM you have to configure OpenClaw so that it thinks PrivacyProxy is the LLM Provider.

```bash
openclaw configure # run configure, select Model -> Custom Provider -> http:<Privacy_Proxy_IP>/v1 -> no API key -> Open AI Endpoint -> Model Alias
```

The Verification checks if the privacy proxy is available if so you can click "continue" and 

```bash
openclaw gateway restart
```

to save changes.

In Session you can select the privacy proxy llm and chat with it. in the logs you should see that PII is recognized and pseudonymized/depseunodymized between OpenClaw and LLM configured in PrivacyProxy settings.

## Docker

PrivacyProxy and the Presidio Analyzer each ship as their own docker-compose project, so they can be built, started and updated independently. They talk to each other securely over a shared external Docker bridge network.

### 1. Create the shared network (once)

```bash
docker network create privacyproxy-net
```

### 2. Start the Presidio Analyzer

```bash
cd Presidio
cp .env.example .env   # adjust PRESIDIO_PORT if needed
docker compose up -d --build
```

This builds the custom Presidio Analyzer image described in the section below and joins it to `privacyproxy-net` under the service name `presidio-analyzer`. The container listens on port `3000` internally and is additionally published on the host via `PRESIDIO_PORT` (default `5002`), e.g. to check `http://localhost:5002` from outside Docker.

### 3. Start PrivacyProxy

```bash
cd PrivacyProxy.Api
cp .env.example .env   # adjust LLM_BASE_URL, LLM_API_KEY, LLM_MODEL, ...
docker compose up -d
```

PrivacyProxy joins the same `privacyproxy-net` network and reaches the Presidio Analyzer at `Presidio__AnalyzerUrl` (default `http://presidio-analyzer:3000`, i.e. the Presidio container's service name and internal port - no host port involved). PrivacyProxy itself is published on the host via `BIND_HOST`/`APP_PORT` (default `127.0.0.1:8080`).

Both `.env.example` files document all available variables. Copy them to `.env` (gitignored) and adjust them to your environment.

## Pull Requests

- [PR into dev](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/compare/dev...FEATURE_BRANCH?template=merge_into_dev_template.md)
- [PR into main](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/compare/main...dev?template=merge_into_main_template.md)

## Microsoft Presidio Analyzer with German supported language

The [`/Presidio`](./Presidio) folder contains a ready-to-use custom build of the Microsoft Presidio Analyzer with German and English language support, consisting of four files:

- `default_recognizers.yaml` - enables the predefined recognizers for `en`/`de` (and a few other languages) plus custom recognizers for German dates, times and amounts (`GermanDateRecognizer`, `GermanTimeRecognizer`, `GermanMoneyRecognizer`)
- `default_analyzer.yaml` - enables `en` and `de` as supported languages for the analyzer
- `default.yaml` - configures the spaCy NLP engine with the `en_core_web_lg` and `de_core_news_lg` models
- `Dockerfile` - builds on `mcr.microsoft.com/presidio-analyzer:latest`, downloads the German and English spaCy models and copies the three config files above into `/app/presidio_analyzer/conf/`

It is started via its own docker-compose project as described in the [Docker](#docker) section above:

```bash
cd Presidio
docker compose up -d --build
```

This builds the image and starts a _Microsoft Presidio Analyzer_ instance that supports German (and English) plus the custom recognizers listed above. It joins the shared `privacyproxy-net` network as `presidio-analyzer` (reachable by PrivacyProxy at `http://presidio-analyzer:3000`) and is additionally published on `http://localhost:5002` (configurable via `PRESIDIO_PORT`) for manual checks.
