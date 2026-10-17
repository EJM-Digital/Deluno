# The end-to-end suite's fixture: a local Torznab feed, a web seed for the torrents it offers, and a read-only look at the
# stack's shared folders. Nothing in it reaches the internet. ffmpeg makes the small real film the run downloads.
FROM node:22-slim@sha256:c3de60bf2f9dd0ac6370e6117950ff62d6e339527e7472301c9c78a017978392

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /fixture
COPY server.mjs /fixture/server.mjs
EXPOSE 8765
ENTRYPOINT ["node", "/fixture/server.mjs"]
