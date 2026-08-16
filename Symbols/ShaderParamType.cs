using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Ruri.ShaderTools;

[JsonConverter(typeof(StringEnumConverter))]
public enum ShaderParamType
{
    Float = 0,
    Int = 1,
    Bool = 2,
    Half = 3,
    Short = 4,
    UInt = 5,
}
