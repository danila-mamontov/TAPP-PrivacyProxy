FROM mcr.microsoft.com/presidio-analyzer:latest

# spaCy provides tokenization/sentence segmentation only -- NER is handled by
# the transformer model. Small models are therefore sufficient.
RUN python -m spacy download de_core_news_sm
RUN python -m spacy download en_core_web_sm

# Presidio's transformer NLP engine + its bridge to spacy-huggingface-pipelines
RUN pip install "presidio-analyzer[transformers]"

# Pre-download the NER model at build time so the container starts without
# needing internet access at runtime and the first request is fast.
RUN python -c "\
from transformers import AutoTokenizer, AutoModelForTokenClassification; \
AutoTokenizer.from_pretrained('Davlan/bert-base-multilingual-cased-ner-hrl'); \
AutoModelForTokenClassification.from_pretrained('Davlan/bert-base-multilingual-cased-ner-hrl')"

COPY default.yaml /app/presidio_analyzer/conf/default.yaml
COPY default_analyzer.yaml /app/presidio_analyzer/conf/default_analyzer.yaml
COPY default_recognizers.yaml /app/presidio_analyzer/conf/default_recognizers.yaml