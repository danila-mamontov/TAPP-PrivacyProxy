## Pull Requests

- [PR into dev](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/compare/dev...FEATURE_BRANCH?template=merge_into_dev_template.md)
- [PR into main](https://github.com/PlueschtierBaum/OpenClaw-PrivacyProxy/compare/main...dev?template=merge_into_main_template.md)

## Microsoft Presidio Analyzer with German supported language
First we have to define three files in the same folder as a dockerfile which uses them:

`default_recognizers.yaml`:
```yaml
supported_languages:
  - en
  - de
global_regex_flags: 26

recognizers:
  - name: CreditCardRecognizer
    supported_languages:
    - language: en
      context: [credit, card, visa, mastercard, cc, amex, discover, jcb, diners, maestro, instapayment]
    - language: es
      context: [tarjeta, credito, visa, mastercard, cc, amex, discover, jcb, diners, maestro, instapayment]
    - language: it
    - language: pl
    type: predefined

  - name: UsBankRecognizer
    supported_languages:
    - en
    type: predefined

  - name: UsLicenseRecognizer
    supported_languages:
    - en
    type: predefined

  - name: UsItinRecognizer
    supported_languages:
    - en
    type: predefined

  - name: UsPassportRecognizer
    supported_languages:
    - en
    type: predefined

  - name: UsSsnRecognizer
    supported_languages:
    - en
    type: predefined

  - name: NhsRecognizer
    supported_languages:
    - en
    type: predefined

  - name: EsNifRecognizer
    supported_languages:
    - es
    type: predefined

  - name: EsNieRecognizer
    supported_languages:
    - es
    type: predefined

  - name: ItDriverLicenseRecognizer
    supported_languages:
    - it
    type: predefined

  - name: ItFiscalCodeRecognizer
    supported_languages:
    - it
    type: predefined

  - name: ItVatCodeRecognizer
    supported_languages:
    - it
    type: predefined

  - name: ItIdentityCardRecognizer
    supported_languages:
    - it
    type: predefined

  - name: ItPassportRecognizer
    supported_languages:
    - it
    type: predefined

  - name: PlPeselRecognizer
    supported_languages:
    - pl
    type: predefined

  - name: CryptoRecognizer
    type: predefined

  - name: DateRecognizer
    type: predefined

  - name: EmailRecognizer
    type: predefined

  - name: IbanRecognizer
    type: predefined

  - name: IpRecognizer
    type: predefined

  - name: MedicalLicenseRecognizer
    type: predefined

  - name: MacAddressRecognizer
    type: predefined

  - name: PhoneRecognizer
    type: predefined

  - name: UrlRecognizer
    type: predefined

  # Deutsche Custom-Recognizer
  - name: GermanDateRecognizer
    supported_languages:
    - de
    type: custom
    supported_entity: DATE_TIME
    patterns:
    - name: german_date
      regex: \b\d{1,2}\.\d{1,2}\.\d{4}\b
      score: 0.85

  - name: GermanTimeRecognizer
    supported_languages:
    - de
    type: custom
    supported_entity: DATE_TIME
    patterns:
    - name: german_time
      regex: \b\d{1,2}:\d{2}\s*(Uhr)?\b
      score: 0.85

  - name: GermanMoneyRecognizer
    supported_languages:
    - de
    type: custom
    supported_entity: MONEY
    patterns:
    - name: german_money
      regex: \d{1,3}(\.\d{3})*(,\d{2})?\s*€
      score: 0.85
```

`default_analyzer.yaml`:
```yaml
supported_languages:
  - en
  - de
default_score_threshold: 0
```

`default.yaml`:
```yaml
nlp_engine_name: spacy
models:
  - lang_code: en
    model_name: en_core_web_lg
  - lang_code: de
    model_name: de_core_news_lg
```

`Dockerfile`:
```Dockerfile
FROM mcr.microsoft.com/presidio-analyzer:latest

RUN python -m spacy download de_core_news_lg
RUN python -m spacy download en_core_web_lg

COPY default.yaml /app/presidio_analyzer/conf/default.yaml
COPY default_analyzer.yaml /app/presidio_analyzer/conf/default_analyzer.yaml
COPY default_recognizers.yaml /app/presidio_analyzer/conf/default_recognizers.yaml
```

navigate to the folder with all four files. Then:
```bash
docker build -t custom-presidio-analyzer .
docker run -d -p 5002:3000 custom-presidio-analyzer
```

Now we have a _Microsoft Presidio Analyzer_ instance running on port 5002 that supports german language and even some custom recognizers.
