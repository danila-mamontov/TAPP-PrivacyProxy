This experiment aims to answer RQ1 of the bachelor's thesis:
> **RQ1.**
> How can personal data contained in messages exchanged between an AI assistant 
> and a cloud-based LLM be reliably pseudonymized prior to transmission and correctly re-identified upon return, 
> independent of the underlying detection component’s accuracy?

To this end, a (potential) solution was implemented within the PrivacyProxy to correctly pseudonymize—and subsequently de-pseudonymize—detected PII.

We use the ai4privacy/pi-masking-200k dataset from HuggingFace.co and a mocked version of Presidio that adopts the contained PII instances exactly as they are; this allows us to verify whether the instances were pseudonymized and subsequently ensure they were de-pseudonymized, without being affected by false negatives or false positives from a real Microsoft Presidio Analyzer.

