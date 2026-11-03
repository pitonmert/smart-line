import argparse
import os


def main():
    parser = argparse.ArgumentParser(prog="smartline")
    commands = parser.add_subparsers(dest="command", required=True)
    web = commands.add_parser("web", help="Run the web/system process")
    web.add_argument(
        "--mock", action="store_true", help="Read-only preview without database or devices"
    )
    web.add_argument("--host", default=os.getenv("WEB_HOST", "127.0.0.1"))
    web.add_argument("--port", type=int, default=int(os.getenv("WEB_PORT", "8000")))
    web.add_argument("--certfile", default=os.getenv("HTTPS_CERT_PATH"))
    web.add_argument("--keyfile", default=os.getenv("HTTPS_KEY_PATH"))
    commands.add_parser("vision", help="Run the image process")
    manage = commands.add_parser("manage", help="Run a Django management command")
    manage.add_argument("args", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command == "vision":
        from smartline.vision.main import main as vision_main

        vision_main()
    else:
        os.environ.setdefault("DJANGO_SETTINGS_MODULE", "smartline.config.settings")
        if args.command == "manage":
            from django.core.management import execute_from_command_line

            execute_from_command_line(["smartline", *args.args])
        else:
            if args.mock:
                os.environ["SMARTLINE_MOCK"] = "true"
            import uvicorn

            uvicorn.run(
                "smartline.config.asgi:application",
                host=args.host,
                port=args.port,
                workers=1,
                lifespan="on",
                ssl_certfile=args.certfile,
                ssl_keyfile=args.keyfile,
                proxy_headers=False,
            )
