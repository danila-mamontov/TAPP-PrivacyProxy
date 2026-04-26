namespace PrivacyProxy.Api.Configuration;

public class PresidioOptions
{
    public required string AnalyzerUrl { get; init; }
    
    public string[] AllowList { get; set; } = [];
    
    public string[] Context { get; set; } = [];
    
    public double ScoreThreshold { get; init; } = 0.4;
    
    public string[] GermanEntityTypes { get; set; } =
        ["PERSON", "LOCATION", "ORGANIZATION"];
    
    public string[] EnglishEntityTypes { get; set; } =
    ["EMAIL_ADDRESS", "PHONE_NUMBER", "IP_ADDRESS", 
        "CREDIT_CARD", "IBAN_CODE", "URL"];
    
    public Dictionary<string, double> GermanEntityThresholds { get; init; } = new()
                                                                             {
                                                                                 { "PERSON",       0.85 },
                                                                                 { "LOCATION",     0.90 },
                                                                                 { "ORGANIZATION", 0.85 }
                                                                             };
    
    public Dictionary<string, double> EnglishEntityThresholds { get; init; } = new()
                                                                               {
                                                                                   { "EMAIL_ADDRESS", 0.4 },
                                                                                   { "PHONE_NUMBER",  0.4 },
                                                                                   { "IP_ADDRESS",    0.4 },
                                                                                   { "CREDIT_CARD",   0.4 },
                                                                                   { "IBAN_CODE",     0.4 },
                                                                                   { "URL",           0.4 }
                                                                               };
}