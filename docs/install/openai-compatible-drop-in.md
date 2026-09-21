## Point a client

Download the installer for your OS from the assets below, then point any OpenAI-compatible client at **one** base URL and send `"model": "auto"`.

**Base URL**

```
https://localhost:47101/v1
```

**Copy-paste**

```bash
export OPENAI_BASE_URL=https://localhost:47101/v1
export OPENAI_API_KEY=not-needed
```

```python
from openai import OpenAI

client = OpenAI(base_url="https://localhost:47101/v1", api_key="not-needed")
client.chat.completions.create(
    model="auto",
    messages=[{"role": "user", "content": "hello"}],
)
```

```bash
curl https://localhost:47101/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"auto","messages":[{"role":"user","content":"hello"}]}'
```

Dashboard: `https://localhost:47104`. Windows/Linux/macOS installers already trust the local CA. Docker and browsers that ignore the OS store: [client TLS setup](https://github.com/davidpizon/TotallyHot-ArcRouter/blob/main/docs/router/client-tls-setup.md). The `OPENAI_API_KEY` value is a placeholder — LLM forwarding is not authenticated; it only satisfies clients that refuse an empty key.
