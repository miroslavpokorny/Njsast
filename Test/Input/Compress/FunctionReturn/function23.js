function func() {
    if (a) {
        if (b) {
            return c;
        } 
        if (a) {
            return d;
        }
        return c;
    } else {
        return d;
    }
    return d;
}