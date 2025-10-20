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

# 4) Open Swagger
Once the app is running, open Swagger at:  
`https://localhost:<port>/swagger`  *(usually 5001/https or 5000/http)*

# 5) Create a job — `POST /jobs`
In Swagger, open **POST /jobs** → **Try it out** → use this body:
```json
{ "url": "https://github.com/dotnet/samples" }

Click Execute and copy the returned jobId

# 6) Check status — GET /jobs/{id}

In Swagger, open GET /jobs/{id}, paste your jobId, then Execute.
Status will move through:

Queued → Fetching → Parsing → Metrics → Summarizing → Saving → Completed

# 7) Get the report — GET /jobs/{id}/report

In Swagger, open GET /jobs/{id}/report and Execute.
You should see JSON with:

metrics (cyclomatic/cognitive: avg & max)

topRiskFiles (top 3 risky files)

summary (overview / risks / actions)

summarySource:

"azure" → summary came from Azure OpenAI

"fallback" → rule-based backup (AI call failed)
