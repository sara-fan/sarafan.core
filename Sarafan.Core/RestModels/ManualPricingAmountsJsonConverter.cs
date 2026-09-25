// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed class ManualPricingAmountsJsonConverter : JsonConverter<Dictionary<ServiceKind, decimal>>
{
    // Preserve the standard reader so previously stored snapshots with enum-name keys remain readable.
    public override Dictionary<ServiceKind, decimal>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
        => JsonSerializer.Deserialize<Dictionary<ServiceKind, decimal>>(ref reader, options);

    public override void Write(Utf8JsonWriter writer, Dictionary<ServiceKind, decimal> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (service, amount) in value)
            writer.WriteNumber(((int)service).ToString(CultureInfo.InvariantCulture), amount);
        writer.WriteEndObject();
    }
}
