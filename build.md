Build & Push the image:
```bash
docker buildx build \
  --platform linux/amd64 \
  --pull \
  -t git.hallboard.ir/team/prod_plane-zulip-bridge:2.1.1 \
  --push \
  .
```