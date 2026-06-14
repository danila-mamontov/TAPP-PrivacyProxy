# PrivacyProxy

PrivacyProxy is a reverse proxy for OpenAI-compatible chat-completion APIs. It sits between an LLM
client (e.g. [OpenClaw](#using-privacyproxy-with-openclaw)) and your LLM provider, detects
personally identifiable information (PII) in outgoing messages using
[Microsoft Presidio](https://microsoft.github.io/presidio/), replaces it with placeholders before
forwarding the request, and restores the original values in the (optionally streamed) response.

[![.NET CI](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/actions/workflows/dotnet.yml/badge.svg)](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/actions/workflows/dotnet.yml)

## Table of Contents

- [How it works](#how-it-works)
- [Getting Started](#getting-started)
- [Running Tests](#running-tests)
- [Using PrivacyProxy with OpenClaw](#using-privacyproxy-with-openclaw)
- [Docker](#docker)
- [Microsoft Presidio Analyzer with German supported language](#microsoft-presidio-analyzer-with-german-supported-language)
- [Pull Requests](#pull-requests)

## How it works

1. A client sends an OpenAI-compatible chat-completion request to PrivacyProxy.
2. Every user message is sent to the Presidio Analyzer - in parallel for German and English - to
   detect PII such as names, locations, organizations, email addresses, phone numbers, IBANs,
   credit card numbers, IP addresses and URLs.
3. Detected entities are replaced with placeholders of the form `[TYPE_HASH16]` (e.g.
   `[PERSON_a6ab9045d1042ef4]`). The mapping between placeholder and original value is kept in
   memory.
4. A system instruction is added that tells the LLM to treat placeholders as opaque values and to
   keep them unchanged, including inside tool-call arguments.
5. The anonymized request is forwarded to the configured LLM provider (any OpenAI-compatible API,
   e.g. [Ollama](https://ollama.com/)).
6. The LLM's response - including streamed (SSE) responses, reasoning tokens and tool calls - is
   scanned for placeholders, which are replaced with their original values before being returned
   to the client.

As a result, the configured LLM provider never sees the original PII, while the client receives a
response with the real values restored.

## Getting Started

PrivacyProxy is under active development and is currently only verified to run with the
`Development` environment. Therefore you have to adjust `appsettings.Development.json`, not
`appsettings.json`, at the moment.

After cloning the project, adjust the configuration, e.g.:

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
  
  "Mapping": {
    "TtlMinutes": 30
  },
  
  "AllowedHosts": "*",
  
  "Urls": "http://*:6000;https://*:6001"
}
```

- The `Presidio` section configures the connection to the Presidio Analyzer as well as the entity
  policy: an allow-list and additional context words used to improve detection, plus per-entity
  score thresholds for German and English.
- The `Llm` section configures the upstream OpenAI-compatible LLM provider that PrivacyProxy
  forwards anonymized requests to.
- The `Mapping` section configures how long (in minutes) PII-to-placeholder mappings are kept in
  memory before expiring. The TTL uses sliding expiration, i.e. it is refreshed on every access.

To run PrivacyProxy locally you need .NET 10:

```bash
cd PrivacyProxy.Api
dotnet restore
dotnet run
```

> For a containerized setup of PrivacyProxy and the Presidio Analyzer, see [Docker](#docker).

## Running Tests

```bash
cd PrivacyProxy.Api.Tests
dotnet restore
dotnet test
```

[dotCover](https://www.jetbrains.com/dotcover/) by JetBrains is fully supported for code coverage;
[Rider](https://www.jetbrains.com/rider/) is the suggested IDE.

## Using PrivacyProxy with OpenClaw

To use PrivacyProxy as a transparent proxy between OpenClaw and your LLM, configure OpenClaw so
that it treats PrivacyProxy as its LLM provider:

```bash
openclaw configure # run configure, select Model -> Custom Provider -> http://<PrivacyProxy-Host>/v1 -> no API key -> OpenAI Endpoint -> Model Alias
```

OpenClaw's verification step checks whether PrivacyProxy is reachable. Once it succeeds, confirm
with "Continue" and apply the change:

```bash
openclaw gateway restart
```

You can now select the PrivacyProxy model in a session and chat with it. PrivacyProxy's logs show
that PII is recognized, anonymized before being sent to the LLM, and deanonymized again in the
response.

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

### Image tags

The CI pipeline automatically builds and publishes the `privacyproxy` image to the GitHub Container Registry on every push to `dev` and `main`:

| Tag                            | Built from | Description                                  |
|--------------------------------|------------|-----------------------------------------------|
| `ghcr.io/plueschtierbaum/privacyproxy:latest`  | `main`     | Latest production-ready build (recommended). |
| `ghcr.io/plueschtierbaum/privacyproxy:stable`  | `main`     | Alias of `latest`, for explicit pinning.     |
| `ghcr.io/plueschtierbaum/privacyproxy:dev`     | `dev`      | Latest development build - may be unstable.  |
| `ghcr.io/plueschtierbaum/privacyproxy:<sha>`   | `dev`/`main` | Immutable build for a specific commit.     |

`PrivacyProxy.Api/docker-compose.yml` uses `:latest` by default; set `PRIVACYPROXY_IMAGE_TAG` (or edit the `image:` line) to pin a different tag.

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

## Pull Requests

- [PR into dev](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/compare/dev...FEATURE_BRANCH?template=merge_into_dev_template.md)
- [PR into main](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/compare/main...dev?template=merge_into_main_template.md)
