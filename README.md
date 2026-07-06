# PrivacyProxy

PrivacyProxy keeps personal data out of the AI you talk to.

It sits between your app (for example [OpenClaw](#using-privacyproxy-with-openclaw)) and any
OpenAI-compatible LLM. Before a message reaches the model, PrivacyProxy finds personal details in it
— names, addresses, emails, phone numbers, IBANs, and more — and swaps them for neutral placeholders.
When the model answers, PrivacyProxy puts the real values back. The model does its job, but it never
sees who you actually are.

[![.NET CI](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/actions/workflows/dotnet.yml/badge.svg)](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/actions/workflows/dotnet.yml)

## Table of Contents

- [How it works](#how-it-works)
- [Getting Started](#getting-started)
- [Running Tests](#running-tests)
- [Using PrivacyProxy with OpenClaw](#using-privacyproxy-with-openclaw)
- [Docker](#docker)
- [The Presidio Analyzer (German + English)](#the-presidio-analyzer-german--english)
- [Pull Requests](#pull-requests)

## How it works

Think of PrivacyProxy as a translator that only your side of the conversation can understand.

1. Your app sends a chat request to PrivacyProxy instead of directly to the LLM.
2. PrivacyProxy scans **every** message for personal data, using
   [Microsoft Presidio](https://microsoft.github.io/presidio/) for both German and English.
3. Each piece of personal data is replaced with a placeholder like `[PERSON_a6ab9045d1042ef4]`.
   PrivacyProxy remembers which placeholder stands for which real value.
4. The cleaned-up request is forwarded to your LLM provider (any OpenAI-compatible API,
   for example [Ollama](https://ollama.com/)).
5. The model's reply — including streamed responses and tool calls — comes back with the
   placeholders still in it.
6. PrivacyProxy swaps the placeholders back to the real values and returns the reply to your app.

The end result: the LLM works with anonymous stand-ins, and you get an answer with the real details
restored — automatically, without changing how your app talks to the model.

## Getting Started

The easiest way to run everything is with [Docker](#docker). If you'd rather run PrivacyProxy
directly, you need [.NET 10](https://dotnet.microsoft.com/):

```bash
cd PrivacyProxy.Api
dotnet restore
dotnet run
```

PrivacyProxy is still in active development and currently runs in the `Development` environment, so
settings go in `appsettings.Development.json` (not `appsettings.json`). A typical configuration:

```json
{
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
  }
}
```

Two sections usually matter:

- **`Presidio`** — where to reach the analyzer and how strict detection should be. Higher thresholds
  mean fewer false positives but more risk of missing something; the allow-list and context words
  fine-tune what gets detected.
- **`Llm`** — the OpenAI-compatible model PrivacyProxy forwards the cleaned request to.

## Running Tests

```bash
cd PrivacyProxy.Api.Tests
dotnet restore
dotnet test
```

Code coverage works out of the box with [dotCover](https://www.jetbrains.com/dotcover/);
[Rider](https://www.jetbrains.com/rider/) is the recommended IDE.

## Using PrivacyProxy with OpenClaw

Point OpenClaw at PrivacyProxy as if it were the LLM itself:

```bash
openclaw configure # Model -> Custom Provider -> http://<PrivacyProxy-Host>/v1 -> no API key -> OpenAI Endpoint -> Model Alias
```

OpenClaw checks that PrivacyProxy is reachable. Once it is, confirm with "Continue", then apply:

```bash
openclaw gateway restart
```

Now pick the PrivacyProxy model in a session and start chatting. PrivacyProxy's logs show personal
data being detected, replaced before it reaches the LLM, and restored in the reply.

## Docker

PrivacyProxy and the Presidio Analyzer are two separate docker-compose projects, so you can start
and update them independently. They talk to each other over a shared Docker network.

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

This builds the analyzer image (see [below](#the-presidio-analyzer-german--english)) and joins it to
`privacyproxy-net` as `presidio-analyzer`. It listens on port `3000` inside Docker and is also
published on the host at `http://localhost:5002` (via `PRESIDIO_PORT`) if you want to check it.

### 3. Start PrivacyProxy

```bash
cd PrivacyProxy.Api
cp .env.example .env   # set LLM_BASE_URL, LLM_API_KEY, LLM_MODEL, ...
docker compose up -d
```

PrivacyProxy joins the same network and reaches the analyzer at `http://presidio-analyzer:3000`. It
is published on the host at `127.0.0.1:8080` (via `BIND_HOST`/`APP_PORT`).

Both `.env.example` files list every available setting. Copy them to `.env` (gitignored) and adjust
as needed.

### Image tags

CI automatically builds and publishes the `privacyproxy` image to the GitHub Container Registry on
every push to `dev` and `main`:

| Tag                                            | Built from   | Description                                  |
|------------------------------------------------|--------------|----------------------------------------------|
| `ghcr.io/plueschtierbaum/privacyproxy:latest`  | `main`       | Latest production-ready build (recommended). |
| `ghcr.io/plueschtierbaum/privacyproxy:stable`  | `main`       | Alias of `latest`, for explicit pinning.     |
| `ghcr.io/plueschtierbaum/privacyproxy:dev`     | `dev`        | Latest development build — may be unstable.  |
| `ghcr.io/plueschtierbaum/privacyproxy:<sha>`   | `dev`/`main` | Immutable build for a specific commit.       |

`PrivacyProxy.Api/docker-compose.yml` uses `:latest` by default; set `PRIVACYPROXY_IMAGE_TAG` to pin
a different tag.

## The Presidio Analyzer (German + English)

The [`/Presidio`](./Presidio) folder contains a ready-to-use build of the Microsoft Presidio Analyzer
with German and English support. It's a small custom image on top of the official
`presidio-analyzer`, adding the German and English spaCy models plus a few extra recognizers for
German dates, times and money amounts.

You start it with its own docker-compose project (see the [Docker](#docker) section):

```bash
cd Presidio
docker compose up -d --build
```

That's all PrivacyProxy needs to detect personal data — no separate setup required.
