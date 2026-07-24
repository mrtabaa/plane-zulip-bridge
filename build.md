Build & Push the image:
```bash
docker buildx build \
  --platform linux/amd64 \
  --pull \
  -t git.hallboard.ir/team/prod_plane-zulip-bridge:2.0.0 \
  --push \
  .
```