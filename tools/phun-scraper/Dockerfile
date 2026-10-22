FROM python:3.12-slim
RUN apt-get update && apt-get install -y --no-install-recommends libimage-exiftool-perl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY phun_scraper.py .
ENV PYTHONUNBUFFERED=1
CMD ["python", "phun_scraper.py"]
