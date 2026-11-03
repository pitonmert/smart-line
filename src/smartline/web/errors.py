class ApiError(Exception):
    def __init__(self, message, code="REQUEST_INVALID", status=400):
        super().__init__(message)
        self.code = code
        self.status = status


def missing(name="Resource"):
    raise ApiError(f"{name} was not found.", "RESOURCE_NOT_FOUND", 404)
