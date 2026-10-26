using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCP.Core.Tools
{
    internal class McpToolSchema
    {
        public McpToolSchema()
        {
        }

        internal static void ValidateDefinition(JsonObject schema)
        {
            ValidateNode(schema, 0);
            if (schema["type"]?.GetValue<string>() != "object")
            {
                throw new ArgumentException("An MCP input schema must describe an object.");
            }
        }

        private static void ValidateNode(JsonObject schema, int depth)
        {
            if (depth > 16)
            {
                throw new ArgumentException("The MCP input schema exceeds the supported depth.");
            }
            string type = schema["type"]?.GetValue<string>();
            if (type != "object" && type != "array" && type != "string" && type != "integer"
                && type != "number" && type != "boolean" && type != "null")
            {
                throw new ArgumentException("MCP input schemas require an explicit supported type.");
            }
            foreach (KeyValuePair<string, JsonNode> field in schema)
            {
                switch (field.Key)
                {
                    case "$schema":
                        if (field.Value?.GetValue<string>() != "https://json-schema.org/draft/2020-12/schema")
                        {
                            throw new ArgumentException("Only JSON Schema draft 2020-12 is supported.");
                        }
                        break;
                    case "type":
                    case "description":
                    case "title":
                    case "default":
                    case "examples":
                        break;
                    case "properties":
                        if (type != "object" || field.Value is not JsonObject properties)
                        {
                            throw new ArgumentException("Schema properties must be an object on an object schema.");
                        }
                        foreach (KeyValuePair<string, JsonNode> property in properties)
                        {
                            if (property.Value is not JsonObject definition)
                            {
                                throw new ArgumentException("Each schema property must have an object definition.");
                            }
                            ValidateNode(definition, depth + 1);
                        }
                        break;
                    case "required":
                        if (type != "object" || field.Value is not JsonArray required)
                        {
                            throw new ArgumentException("Schema required must be an array on an object schema.");
                        }
                        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (JsonNode name in required)
                        {
                            if (name == null || name.GetValueKind() != JsonValueKind.String
                                || names.Add(name.GetValue<string>()) == false)
                            {
                                throw new ArgumentException("Required schema property names must be unique strings.");
                            }
                        }
                        break;
                    case "additionalProperties":
                        if (type != "object" || field.Value == null
                            || (field.Value.GetValueKind() != JsonValueKind.True && field.Value.GetValueKind() != JsonValueKind.False))
                        {
                            throw new ArgumentException("Schema additionalProperties must be a Boolean on an object schema.");
                        }
                        break;
                    case "items":
                        if (type != "array" || field.Value is not JsonObject items)
                        {
                            throw new ArgumentException("Schema items must be an object on an array schema.");
                        }
                        ValidateNode(items, depth + 1);
                        break;
                    case "enum":
                        if (field.Value is not JsonArray values || values.Count == 0)
                        {
                            throw new ArgumentException("Schema enum must be a nonempty array.");
                        }
                        break;
                    case "minLength":
                    case "maxLength":
                    case "minItems":
                    case "maxItems":
                        bool appliesToString = field.Key.EndsWith("Length", StringComparison.Ordinal);
                        if ((appliesToString == true && type != "string") || (appliesToString == false && type != "array")
                            || field.Value == null || int.TryParse(field.Value.ToJsonString(), NumberStyles.None,
                                CultureInfo.InvariantCulture, out _) == false)
                        {
                            throw new ArgumentException("Schema length and item limits must be nonnegative integers on the matching type.");
                        }
                        break;
                    case "minimum":
                    case "maximum":
                        if ((type != "number" && type != "integer") || field.Value == null
                            || TryNumber(field.Value, out _) == false)
                        {
                            throw new ArgumentException("Schema numeric limits must be finite numbers on a numeric type.");
                        }
                        break;
                    default:
                        throw new NotSupportedException("Unsupported MCP input schema keyword: " + field.Key);
                }
            }
            if (type == "object" && schema["required"] is JsonArray requiredNames
                && schema["additionalProperties"]?.GetValue<bool>() == false)
            {
                JsonObject properties = schema["properties"] as JsonObject;
                foreach (JsonNode required in requiredNames)
                {
                    if (properties == null || properties.ContainsKey(required.GetValue<string>()) == false)
                    {
                        throw new ArgumentException("A required property must be defined when additional properties are disabled.");
                    }
                }
            }
            EnsureLimitOrder(schema, "minLength", "maxLength");
            EnsureLimitOrder(schema, "minItems", "maxItems");
            EnsureLimitOrder(schema, "minimum", "maximum");
        }

        private static void EnsureLimitOrder(JsonObject schema, string minimum, string maximum)
        {
            if (schema[minimum] != null && schema[maximum] != null
                && ReadNumber(schema[minimum]) > ReadNumber(schema[maximum]))
            {
                throw new ArgumentException("The schema minimum exceeds its maximum: " + minimum);
            }
        }

        internal static void ValidateArguments(JsonObject schema, JsonNode value, string path)
        {
            string type = schema["type"].GetValue<string>();
            bool matches = false;
            if (type == "null")
            {
                matches = value == null;
            }
            else if (value != null)
            {
                switch (type)
                {
                    case "object":
                        matches = value is JsonObject;
                        break;
                    case "array":
                        matches = value is JsonArray;
                        break;
                    case "string":
                        matches = value.GetValueKind() == JsonValueKind.String;
                        break;
                    case "boolean":
                        matches = value.GetValueKind() == JsonValueKind.True || value.GetValueKind() == JsonValueKind.False;
                        break;
                    case "number":
                        matches = TryNumber(value, out _);
                        break;
                    case "integer":
                        matches = TryNumber(value, out double number) == true && Math.Truncate(number) == number;
                        break;
                }
            }
            if (matches == false)
            {
                throw new ArgumentException(path + " must have type " + type + ".");
            }
            if (schema["enum"] is JsonArray alternatives && alternatives.Any(item => JsonNode.DeepEquals(item, value)) == false)
            {
                throw new ArgumentException(path + " must be one of the allowed values.");
            }
            if (value is JsonObject fields)
            {
                if (schema["required"] is JsonArray required)
                {
                    foreach (JsonNode name in required)
                    {
                        if (fields.ContainsKey(name.GetValue<string>()) == false)
                        {
                            throw new ArgumentException(path + "." + name.GetValue<string>() + " is required.");
                        }
                    }
                }
                JsonObject properties = schema["properties"] as JsonObject;
                foreach (KeyValuePair<string, JsonNode> field in fields)
                {
                    if (properties != null && properties[field.Key] is JsonObject definition)
                    {
                        ValidateArguments(definition, field.Value, path + "." + field.Key);
                    }
                    else if (schema["additionalProperties"]?.GetValue<bool>() == false)
                    {
                        throw new ArgumentException(path + "." + field.Key + " is not supported.");
                    }
                }
            }
            if (value is JsonArray array)
            {
                ValidateRange(schema, array.Count, "minItems", "maxItems", path);
                if (schema["items"] is JsonObject items)
                {
                    for (int index = 0; index < array.Count; index++)
                    {
                        ValidateArguments(items, array[index], path + "[" + index + "]");
                    }
                }
            }
            if (type == "string")
            {
                int length = value.GetValue<string>().EnumerateRunes().Count();
                ValidateRange(schema, length, "minLength", "maxLength", path);
            }
            if (type == "number" || type == "integer")
            {
                ValidateRange(schema, ReadNumber(value), "minimum", "maximum", path);
            }
        }

        private static void ValidateRange(JsonObject schema, double value, string minimum, string maximum, string path)
        {
            if (schema[minimum] != null && value < ReadNumber(schema[minimum]))
            {
                throw new ArgumentException(path + " is below " + minimum + ".");
            }
            if (schema[maximum] != null && value > ReadNumber(schema[maximum]))
            {
                throw new ArgumentException(path + " exceeds " + maximum + ".");
            }
        }

        private static bool TryNumber(JsonNode value, out double number)
        {
            number = 0;
            return value.GetValueKind() == JsonValueKind.Number
                && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) == true
                && double.IsFinite(number) == true;
        }

        private static double ReadNumber(JsonNode value)
        {
            return double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
