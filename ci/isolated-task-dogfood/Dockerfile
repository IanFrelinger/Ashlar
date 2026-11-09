ARG BASE_IMAGE=python:3.13-slim
FROM ${BASE_IMAGE}
WORKDIR /app
COPY parser.py /app/parser.py
RUN chown 65532:65532 /app
USER 65532:65532
