# Dev-server image for the Angular console. Compose bind-mounts the source over /app for live
# editing, but keeps this image's node_modules (via an anonymous volume) so a fresh clone needs
# no host Node. Production would build (`ng build`) and serve the static bundle from nginx instead.
FROM node:22

WORKDIR /app

# Install deps first so they're cached independently of source changes.
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .

EXPOSE 4200
ENV NG_CLI_ANALYTICS=false

# --poll: bind-mounted file changes don't always raise inotify events in a container, so watch by
# polling. --host 0.0.0.0 makes the dev server reachable from outside the container.
CMD ["npm", "start", "--", "--host", "0.0.0.0", "--port", "4200", "--poll", "1000"]
