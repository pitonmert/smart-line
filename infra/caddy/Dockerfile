FROM node:22-alpine AS dashboard-build
WORKDIR /dashboard
COPY SmartLine.Dashboard/package.json SmartLine.Dashboard/package-lock.json ./
RUN npm ci
COPY SmartLine.Dashboard/ ./
RUN npm run build

FROM caddy:2.10-alpine
COPY infra/caddy/Caddyfile /etc/caddy/Caddyfile
COPY --from=dashboard-build /dashboard/dist /srv
