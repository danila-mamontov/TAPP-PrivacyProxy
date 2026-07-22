Some Experiments require a openclaw instance.

we set uour up by following steps:

go into the Experiments-Root-Folder and git clone the openclaw's repo. our version was `2026.7.1`.

After that make sure a LLM is acvailable. We recommend installing _ollama_ on your system, pulling the LLMs you want to test and make them reachable via host.docker.internal bridge for containers.

```bash
cd openclaw
./scripts/docker/setup.sh
```

Set up the LLM, don't set up anything else.

Go into `~/.openclaw/openclaw.json` and configure PrivacyProxy and mcp. It should look like this:

```json
{
  "wizard": {
    "securityAcknowledgedAt": "2026-07-20T14:17:34.468Z",
    "lastRunAt": "2026-07-20T14:19:20.136Z",
    "lastRunVersion": "2026.7.2",
    "lastRunCommand": "onboard",
    "lastRunMode": "local"
  },
  "agents": {
    "defaults": {
      "workspace": "/home/node/.openclaw/workspace",
      "model": {
        "primary": "llm-direct/kimi-k2.6:cloud"
      },
      "models": {
        "llm-direct/kimi-k2.6:cloud": {},
        "privacyproxy/kimi-k2.6:cloud": {}
      },
      "sandbox": {
        "mode": "off"
      }
    }
  },
  "gateway": {
    "mode": "local",
    "auth": {
      "mode": "token",
      "token": "<token>"
    },
    "port": 18789,
    "bind": "lan",
    "tailscale": {
      "mode": "off",
      "resetOnExit": false
    },
    "controlUi": {
      "allowInsecureAuth": true,
      "allowedOrigins": [
        "http://localhost:18789",
        "http://127.0.0.1:18789"
      ]
    },
    "nodes": {
      "denyCommands": [
        "camera.snap",
        "camera.clip",
        "screen.record",
        "computer.act",
        "contacts.add",
        "calendar.add",
        "reminders.add",
        "sms.send",
        "sms.search",
        "health.summary"
      ]
    }
  },
  "tools": {
    "profile": "coding"
  },
  "models": {
    "mode": "merge",
    "providers": {
      "llm-direct": {
        "baseUrl": "http://host.docker.internal:11434/v1",
        "api": "openai-completions",
        "models": [
          {
            "id": "kimi-k2.6:cloud",
            "name": "kimi-k2.6:cloud (direct)",
            "contextWindow": 128000,
            "maxTokens": 4096,
            "input": [
              "text"
            ],
            "cost": {
              "input": 0,
              "output": 0,
              "cacheRead": 0,
              "cacheWrite": 0
            },
            "reasoning": false
          }
        ]
      },
      "privacyproxy": {
        "baseUrl": "http://host.docker.internal:8080/v1",
        "api": "openai-completions",
        "models": [
          {
            "id": "kimi-k2.6:cloud",
            "name": "kimi-k2.6:cloud (via PrivacyProxy)",
            "contextWindow": 128000,
            "maxTokens": 4096,
            "input": [
              "text"
            ],
            "cost": {
              "input": 0,
              "output": 0,
              "cacheRead": 0,
              "cacheWrite": 0
            },
            "reasoning": false
          }
        ]
      }
    }
  },
  "mcp": {
    "servers": {
      "rq2tools": {
        "url": "http://host.docker.internal:3100/mcp",
        "transport": "streamable-http"
      }
    }
  },
  "skills": {
    "install": {
      "nodeManager": "npm"
    }
  },
  "hooks": {
    "internal": {
      "entries": {
        "session-memory": {
          "enabled": true
        }
      }
    }
  },
  "meta": {
    "migrations": {
      "modelPolicyAllowlist": true
    },
    "lastTouchedVersion": "2026.7.2",
    "lastTouchedAt": "2026-07-20T14:19:45.944Z"
  }
}
```

restart the openclaw docker container. in our case: 

```bash
docker restart openclaw-openclaw-gateway-1
```


