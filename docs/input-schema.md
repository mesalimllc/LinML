# LinML Input Schema

The `--inputs` JSON file for `linml run` follows this schema.

## Schema

```json
{
  "$schema": "https://linml.dev/schemas/input-v1.json",
  "tensors": [
    {
      "name": "input",
      "type": "float32",
      "shape": [3, 2],
      "data": [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
    }
  ]
}
```

## Fields

### Root

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `$schema` | string | No | Schema identifier URL |
| `tensors` | array | Yes | List of input tensors |

### Tensor

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Tensor name (must match model input name) |
| `type` | string | Yes | Data type: `float32`, `float16`, `int64`, `int32` |
| `shape` | array[int] | Yes | Tensor dimensions |
| `data` | array[number] | Yes | Flat tensor data (row-major) |

## Validation Rules

1. **Data length:** `data.length` must equal the product of `shape` dimensions.
2. **Type matching:** `type` must match the model's expected input type.
3. **Name matching:** `name` must match a model input name.
4. **No silent coercion:** Incompatible types or shapes cause an error (exit code 1 or 2).

## Examples

### Single float32 tensor
```json
{
  "tensors": [
    {
      "name": "input",
      "type": "float32",
      "shape": [1, 3, 224, 224],
      "data": [0.1, 0.2, ...]
    }
  ]
}
```

### Multiple tensors
```json
{
  "tensors": [
    {
      "name": "input_ids",
      "type": "int64",
      "shape": [1, 128],
      "data": [101, 2023, 2003, ...]
    },
    {
      "name": "attention_mask",
      "type": "int64",
      "shape": [1, 128],
      "data": [1, 1, 1, ...]
    }
  ]
}
```

## Output Schema

The `linml run --json` output follows the `InferenceResult` schema in `diagnostics-schema.md`.
