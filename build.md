Build and push the image (set the release tag first):
```bash
IMAGE_TAG=5.4.0
docker buildx build \
  --platform linux/amd64 \
  --pull \
  -t git.hallboard.ir/team/prod_plane-zulip-bridge:${IMAGE_TAG} \
  --push \
  .
```
