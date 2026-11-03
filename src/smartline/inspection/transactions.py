from contextlib import contextmanager

from django.db import connection, transaction


@contextmanager
def inspection_transaction():
    with transaction.atomic():
        with connection.cursor() as cursor:
            cursor.execute("SELECT pg_advisory_xact_lock(20261024)")
        yield
