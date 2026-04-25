using System.Text.Json.Serialization;

namespace PrivacyProxy.Api.Models.Enums;

public enum Language
{
    [JsonStringEnumMemberName("en")]
    English,
    [JsonStringEnumMemberName("de")]
    German
}