function func() {
    if (a) {
        if (b) {
            return c;
        } else {
            return d;
        }
        return c;
    } else {
        return d;
    }
    return d;
}