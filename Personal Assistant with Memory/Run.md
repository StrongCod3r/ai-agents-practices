# Run App

```bash
# Launch the assistant
docker compose up -d --wait
docker compose ps

# Stop
docker compose down

# Configure
$env:OPENROUTER_API_KEY = 'tu-clave'
$env:Agent__Model = 'identificador-del-modelo'
$env:Embeddings__Model = 'openai/text-embedding-3-small'
$env:Embeddings__Dimensions = '1536'

# Primera ejecución: crear el esquema
dotnet run --project src/MemoryAssistant.Api --no-launch-profile -- --migrate

# Arrancar API y chat
dotnet run --project src/MemoryAssistant.Api --launch-profile http

# Si tienes recuerdos anteriores, reindexa con la API arrancada:
Invoke-RestMethod -Method Post -Uri 'http://localhost:5281/api/memories/reindex'
```
