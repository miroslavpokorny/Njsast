!function(t) {
    "use strict";
    var _, n, e, r, a, s, o;
    _ = Object.setPrototypeOf || {
        __proto__: []
    } instanceof Array && function(t, _) {
        t.__proto__ = _;
    } || function(t, _) {
        var n;
        for (n in _) if (_.hasOwnProperty(n)) t[n] = _[n];
    };
    n = Object.assign || function(t) {
        var _, n, e, r;
        for (_ = 1, n = arguments.length; _ < n; _++) {
            e = arguments[_];
            for (r in e) if (Object.prototype.hasOwnProperty.call(e, r)) t[r] = e[r];
        }
        return t;
    };
    e;
    r;
    a;
    s = !1;
    o = 42;
    console.log(o);
}();

