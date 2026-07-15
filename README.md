# Universal Local RAG Context Engine

> **100% local, offline, privacy-first** semantic context retrieval for massive enterprise repositories.

## Stack

| Layer | Technology |
|---|---|
| Language | C# 12/13 (.NET 10) |
| AI Orchestrator | Microsoft Semantic Kernel 1.36.0 |
| Embedding Engine | ONNX Runtime 1.21.0 + all-MiniLM-L6-v2 |
| Vector DB | Qdrant 1.14.1 (Docker) |
| CLI UI | Spectre.Console 0.49.1 + System.CommandLine |

## Quick Start

### 1. Start Qdrant

```bash
docker compose -f infra/docker-compose.yml up -d
```

### 2. Download the embedding model

```bash
chmod +x infra/download-model.sh
bash infra/download-model.sh
```

### 3. Build the solution

```bash
dotnet build
```

### 4. Run the CLI (Sprint 1+)

```bash
dotnet run --project src/RagEngine.Cli -- rag ingest /path/to/repo
dotnet run --project src/RagEngine.Cli -- rag search "how is authentication handled"
```

## Solution Structure

```
rag.context.engine/
├── RagEngine.slnx                  # .NET 10 solution (XML format)
├── infra/
│   ├── docker-compose.yml          # Qdrant container
│   └── download-model.sh           # ONNX model downloader
├── models/                         # (gitignored) ONNX model files
│   └── all-MiniLM-L6-v2/
│       ├── model.onnx
│       ├── tokenizer.json
│       └── ...
└── src/
    ├── RagEngine.Core/             # Class library — all business logic
    │   ├── Abstractions/           # IIngestionScanner, IVectorizationBrain, etc.
    │   ├── Domain/                 # CodeChunk, ScoredChunk, IngestionResult, etc.
    │   ├── Infrastructure/
    │   │   ├── Scanning/           # FileSystemIngestionScanner (S1)
    │   │   ├── Vectorization/      # OnnxVectorizationBrain (S1)
    │   │   └── VectorStore/        # QdrantSemanticRetriever (S1)
    │   ├── Pipeline/               # ChannelIngestionPipeline (S1)
    │   └── Extensions/             # AddRagEngineCore() DI registration
    └── RagEngine.Cli/              # Console entry point
        ├── Commands/               # IngestCommand, SearchCommand (S1/S2)
        └── Program.cs
```

## Sprint Roadmap

| Sprint | Days | Goal |
|--------|------|------|
| **S0** | ~4 | ✅ Walking Skeleton (this commit) |
| **S1** | ~7 | `rag ingest <path>` with Spectre.Console UI |
| **S2** | ~5 | `rag search <query>` with Markdown/JSON output |
| **S3** | ~4 | Multi-language, incremental re-index, `rag status` |
| **S4** | ~5 | `RagContextPlugin` for Semantic Kernel agents |
| **S5** | ~4 | Hardening: Serilog, Polly, metrics, `rag doctor` |

## Security Note

`Microsoft.SemanticKernel.Core` has an open advisory (GHSA-2ww3-72rp-wpp4).
This engine operates 100% offline with no LLM API calls from the Core library,
so the attack surface is effectively zero for local-only deployments.
Monitor the upstream SK repository for a patched release.
