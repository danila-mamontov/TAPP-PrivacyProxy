FROM mcr.microsoft.com/presidio-analyzer:latest

RUN python -m spacy download de_core_news_lg
RUN python -m spacy download en_core_web_lg

COPY default.yaml /app/presidio_analyzer/conf/default.yaml
COPY default_analyzer.yaml /app/presidio_analyzer/conf/default_analyzer.yaml
COPY default_recognizers.yaml /app/presidio_analyzer/conf/default_recognizers.yaml