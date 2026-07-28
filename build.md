Build & Push the image:
```bash
docker buildx build \
  --platform linux/amd64 \
  --pull \
  -t git.hallboard.ir/team/prod_plane-zulip-bridge:1.3.0 \
  --push \
  .
```