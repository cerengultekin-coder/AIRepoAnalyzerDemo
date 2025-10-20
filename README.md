# AI Repo Analyzer – Demo


This is a small demo for a Repo Analyzer. API takes a repo URL, puts a job on a queue. A background worker clones the repo, parses code files, calculates simple metrics, and asks Azure OpenAI for a JSON summary (overview/risks/actions). You can test it via Swagger.

## Requirements
- .NET 8 SDK
- Git installed (for `git clone`)
- Azure OpenAI (endpoint, key, deployment name)

## Quick Start
```bash
# 1) restore & build
dotnet restore
dotnet build

# 2) set secrets (use your own values)
cd src/RepoAnalyzerDemo
dotnet user-secrets set "AZURE_OPENAI_ENDPOINT"   "https://<your>.cognitiveservices.azure.com/"
dotnet user-secrets set "AZURE_OPENAI_APIKEY"     "<your-key>"
dotnet user-secrets set "AZURE_OPENAI_DEPLOYMENT" "gpt-4o-mini"

# 3) run
dotnet run
