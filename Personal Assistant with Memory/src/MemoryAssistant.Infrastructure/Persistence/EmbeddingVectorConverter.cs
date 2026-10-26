using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

namespace MemoryAssistant.Infrastructure.Persistence;

// The domain owns plain numeric data. Only this adapter knows the PostgreSQL vector type.
public sealed class EmbeddingVectorConverter()
    : ValueConverter<float[], Vector>(values => new Vector(values), vector => vector.ToArray());
