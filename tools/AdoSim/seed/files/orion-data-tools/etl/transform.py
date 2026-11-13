def normalize(row: dict) -> dict:
    return {key.strip().lower(): value for key, value in row.items()}
